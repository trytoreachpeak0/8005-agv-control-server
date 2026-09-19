using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class JourneyStopRowConfiguration : IEntityTypeConfiguration<JourneyStopRow>
{
    public void Configure(EntityTypeBuilder<JourneyStopRow> builder)
    {
        builder.ToTable("JourneyStops");
        // The stable identity is the key, never the sequence: an en-route addition re-sequences the stops after it,
        // and a key that moved with them would re-identify every one (the MVP defect specification 5.2 names).
        builder.HasKey(row => row.StopId);
        // Not unique on purpose: re-sequencing rewrites several rows in one save, and a unique index would refuse the
        // intermediate states SQLite checks statement by statement.
        builder.HasIndex(row => new { row.JourneyId, row.Sequence });
    }
}
