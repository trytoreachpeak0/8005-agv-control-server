using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class JourneyDemandRowConfiguration : IEntityTypeConfiguration<JourneyDemandRow>
{
    public void Configure(EntityTypeBuilder<JourneyDemandRow> builder)
    {
        // A demand belongs to at most one journey, ever. The unique index says so rather than
        // leaving it to the callers that look a demand's journey up.
        builder.HasKey(row => new { row.JourneyId, row.DemandId });
        builder.HasIndex(row => row.DemandId).IsUnique();
        builder.HasIndex(row => row.LoadSlotOperationAttemptId).IsUnique();
        builder.HasIndex(row => row.UnloadSlotOperationAttemptId).IsUnique();
        builder.Property(row => row.State).HasConversion<string>();
    }
}
