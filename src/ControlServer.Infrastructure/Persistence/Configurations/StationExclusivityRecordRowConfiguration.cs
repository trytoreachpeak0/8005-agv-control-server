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
        // At most one unreleased passage per station, agreeing with StationExclusivities' key in the same save.
        builder.HasIndex(row => new { row.MapId, row.StationId }).IsUnique().HasFilter("ReleasedAt IS NULL");
        builder.HasIndex(row => row.VehicleKey);
        builder.HasIndex(row => row.JourneyId);
    }
}
