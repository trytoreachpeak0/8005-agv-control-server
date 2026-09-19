using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class JourneyDemandRowConfiguration : IEntityTypeConfiguration<JourneyDemandRow>
{
    public void Configure(EntityTypeBuilder<JourneyDemandRow> builder)
    {
        builder.ToTable("JourneyDemands");
        builder.HasKey(row => new { row.JourneyId, row.DemandId });
        // A demand belongs to at most one journey at a time. A removed membership steps out of the scope rather than
        // being deleted, so a demand released for redispatch (control-server#215) can join a second journey.
        builder.HasIndex(row => row.DemandId)
            .IsUnique()
            .HasFilter("RemovedAt IS NULL");
    }
}
