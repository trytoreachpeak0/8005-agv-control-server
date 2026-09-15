using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class DispatchZoneAreaAssignmentRowConfiguration
    : IEntityTypeConfiguration<DispatchZoneAreaAssignmentRow>
{
    public void Configure(EntityTypeBuilder<DispatchZoneAreaAssignmentRow> builder)
    {
        builder.ToTable("DispatchZoneAreaAssignments");
        // One AREA appears at most once per version: an AREA with two zones or two sides is exactly
        // the duplicate REQ-0350 rejects, and the key says so rather than leaving it to the importer.
        builder.HasKey(row => new { row.Version, row.Area });
        builder.Property(row => row.Version).ValueGeneratedNever();
    }
}
