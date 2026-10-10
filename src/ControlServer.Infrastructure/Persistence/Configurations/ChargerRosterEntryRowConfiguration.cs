using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargerRosterEntryRowConfiguration : IEntityTypeConfiguration<ChargerRosterEntryRow>
{
    public void Configure(EntityTypeBuilder<ChargerRosterEntryRow> builder)
    {
        builder.ToTable("ChargerRosterEntries");
        builder.HasKey(row => new { row.Version, row.MapId, row.StationId });
        builder.Property(row => row.Version).ValueGeneratedNever();
        builder.Property(row => row.MapId).ValueGeneratedNever();
        builder.Property(row => row.StationId).ValueGeneratedNever();
    }
}
