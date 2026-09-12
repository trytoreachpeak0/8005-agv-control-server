using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SlotIoBindingRowConfiguration : IEntityTypeConfiguration<SlotIoBindingRow>
{
    public void Configure(EntityTypeBuilder<SlotIoBindingRow> builder)
    {
        builder.ToTable("SlotIoBindings");
        builder.HasKey(row => row.SlotIoBindingId);
        // One binding per slot per vehicle per model version per version. Superseded versions stay
        // in the table as the record of what the car was wired as.
        builder
            .HasIndex(row => new { row.AgvId, row.SlotModelVersionId, row.PhysicalSlotNumber, row.Version })
            .IsUnique();
    }
}
