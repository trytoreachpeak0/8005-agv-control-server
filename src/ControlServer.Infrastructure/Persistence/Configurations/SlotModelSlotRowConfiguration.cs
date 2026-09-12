using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SlotModelSlotRowConfiguration : IEntityTypeConfiguration<SlotModelSlotRow>
{
    public void Configure(EntityTypeBuilder<SlotModelSlotRow> builder)
    {
        builder.ToTable("SlotModelSlots");
        builder.HasKey(row => new { row.SlotModelVersionId, row.PhysicalSlotNumber });
        builder.HasIndex(row => row.SlotTemplateId);
    }
}
