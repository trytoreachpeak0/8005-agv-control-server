using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SlotConfigurationVerificationRowConfiguration
    : IEntityTypeConfiguration<SlotConfigurationVerificationRow>
{
    public void Configure(EntityTypeBuilder<SlotConfigurationVerificationRow> builder)
    {
        builder.ToTable("SlotConfigurationVerifications");
        builder.HasKey(row => row.VerificationId);
        builder
            .HasIndex(row => new { row.AgvId, row.SlotModelVersionId, row.PhysicalSlotNumber })
            .IsUnique();
    }
}
