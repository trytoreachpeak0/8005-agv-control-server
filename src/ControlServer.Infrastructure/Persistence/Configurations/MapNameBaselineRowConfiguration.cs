using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class MapNameBaselineRowConfiguration : IEntityTypeConfiguration<MapNameBaselineRow>
{
    public void Configure(EntityTypeBuilder<MapNameBaselineRow> builder)
    {
        builder.ToTable("MapNameBaselines");
        // One baseline per Map, keyed by the stable id: a name is never an identity (REQ-0341).
        builder.HasKey(row => row.MapId);
        builder.Property(row => row.MapId).ValueGeneratedNever();
    }
}
