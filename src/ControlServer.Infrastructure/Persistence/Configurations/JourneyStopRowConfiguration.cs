using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class JourneyStopRowConfiguration : IEntityTypeConfiguration<JourneyStopRow>
{
    public void Configure(EntityTypeBuilder<JourneyStopRow> builder)
    {
        builder.HasKey(row => new { row.JourneyId, row.Sequence });
        builder.HasIndex(row => row.UpperId).IsUnique();
        builder.HasIndex(row => row.MovementLegId).IsUnique();
    }
}
