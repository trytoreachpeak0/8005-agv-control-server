using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TransportDemandCompletionRowConfiguration
    : IEntityTypeConfiguration<TransportDemandCompletionRow>
{
    public void Configure(EntityTypeBuilder<TransportDemandCompletionRow> builder)
    {
        builder.HasKey(row => row.TransportDemandKey);
        builder.HasIndex(row => row.DemandId).IsUnique();
    }
}
