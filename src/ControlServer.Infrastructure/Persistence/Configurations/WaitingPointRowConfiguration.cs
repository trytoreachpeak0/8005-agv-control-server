using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class WaitingPointRowConfiguration : IEntityTypeConfiguration<WaitingPointRow>
{
    public void Configure(EntityTypeBuilder<WaitingPointRow> builder)
    {
        builder.ToTable("WaitingPoints");
        builder.HasKey(row => new { row.Version, row.MapId, row.StationId });
        builder.Property(row => row.Version).ValueGeneratedNever();
        builder.Property(row => row.MapId).ValueGeneratedNever();
        builder.Property(row => row.StationId).ValueGeneratedNever();
    }
}
