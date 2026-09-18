using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class StructuralDispatchBlockRowConfiguration : IEntityTypeConfiguration<StructuralDispatchBlockRow>
{
    public void Configure(EntityTypeBuilder<StructuralDispatchBlockRow> builder)
    {
        builder.ToTable("StructuralDispatchBlocks");
        // Deduplicated by demand and reason (REQ-0210): the key is the deduplication, so a second
        // row for the same reason cannot exist however the writer is called.
        builder.HasKey(row => new { row.DemandId, row.ReasonCode });
        builder.HasIndex(row => row.ClearedAt);
        builder.HasIndex(row => row.TransportDemandKey);
    }
}
