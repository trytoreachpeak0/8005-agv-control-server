using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TransportDemandSuppressionRowConfiguration : IEntityTypeConfiguration<TransportDemandSuppressionRow>
{
    public void Configure(EntityTypeBuilder<TransportDemandSuppressionRow> builder)
    {
        builder.ToTable("TransportDemandSuppressions");
        // The first terminal outcome for a demand identity -- business key and leg (control-server#911) -- wins and is never
        // rewritten: the key makes a second one a conflict rather than a second row.
        builder.HasKey(row => new { row.TransportDemandKey, row.TransportLeg });
        builder.HasIndex(row => row.DemandId);
    }
}
