using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TaskTypeStationCatalogChangeRowConfiguration
    : IEntityTypeConfiguration<TaskTypeStationCatalogChangeRow>
{
    public void Configure(EntityTypeBuilder<TaskTypeStationCatalogChangeRow> builder)
    {
        builder.ToTable("TaskTypeStationCatalogChanges");
        builder.HasKey(row => row.ChangeId);
        // One record per station per catalog revision: seeing the same revision again is the same change.
        builder.HasIndex(row => new { row.MapId, row.StationRiotId, row.CatalogRevision }).IsUnique();
    }
}
