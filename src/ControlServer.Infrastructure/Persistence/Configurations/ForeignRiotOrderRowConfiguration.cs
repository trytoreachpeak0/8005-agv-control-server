using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ForeignRiotOrderRowConfiguration : IEntityTypeConfiguration<ForeignRiotOrderRow>
{
    public void Configure(EntityTypeBuilder<ForeignRiotOrderRow> builder)
    {
        builder.ToTable("ForeignRiotOrders");
        // One RIoT order is found, cancelled and ends once: handling it twice lands on this key.
        builder.HasKey(row => row.RiotOrderId);
        // Every round, dispatch asks which vehicles are held by a foreign order; this keeps that a lookup.
        builder.HasIndex(row => new { row.AgvId, row.State });
        builder.HasIndex(row => row.CancelCommandAuditId).IsUnique();
    }
}
