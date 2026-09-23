using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class OwnOrderRebuildRowConfiguration : IEntityTypeConfiguration<OwnOrderRebuildRow>
{
    public void Configure(EntityTypeBuilder<OwnOrderRebuildRow> builder)
    {
        builder.ToTable("OwnOrderRebuilds");
        // Derived from the ended order's upperId: one RIoT order ends once, so the same ending handled twice lands on this key.
        builder.HasKey(row => row.RebuildId);
        builder.HasIndex(row => row.EndedUpperId).IsUnique();
        builder.HasIndex(row => row.NewUpperId).IsUnique();
        builder.HasIndex(row => new { row.JourneyId, row.StopId });
        builder.HasIndex(row => row.DemandId);
        // Every inbound onboard message asks whether this vehicle has a cargo snapshot to request (REQ-0362); this index keeps
        // that question a lookup when, as almost always, nothing waits.
        builder.HasIndex(row => new { row.AgvId, row.State });
        builder.Property(row => row.State).IsConcurrencyToken();
    }
}
