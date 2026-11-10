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
        // Every membership of a demand, removed ones included, by generation (control-server#881): what
        // DemandJourneyLookup.ReleasedForRedispatch asks of the removed memberships and of "a later generation". The partial
        // index above cannot answer either, and without this one both were full scans, twice or more every dispatch round.
        builder.HasIndex(row => new { row.DemandId, row.DispatchGeneration });
    }
}
