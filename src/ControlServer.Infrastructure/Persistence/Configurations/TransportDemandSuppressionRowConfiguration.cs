using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TransportDemandSuppressionRowConfiguration : IEntityTypeConfiguration<TransportDemandSuppressionRow>
{
    public void Configure(EntityTypeBuilder<TransportDemandSuppressionRow> builder)
    {
        builder.ToTable("TransportDemandSuppressions");
        // The first terminal outcome for a business key wins and is never rewritten: the key makes a second one a
        // conflict rather than a second row.
        builder.HasKey(row => row.TransportDemandKey);
        builder.HasIndex(row => row.DemandId);
    }
}
