using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TransportDemandSuppressionRowConfiguration
    : IEntityTypeConfiguration<TransportDemandSuppressionRow>
{
    public void Configure(EntityTypeBuilder<TransportDemandSuppressionRow> builder)
    {
        builder.HasKey(row => row.TransportDemandKey);
    }
}
