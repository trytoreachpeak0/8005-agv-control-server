using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SlotConfigurationActivationRowConfiguration
    : IEntityTypeConfiguration<SlotConfigurationActivationRow>
{
    public void Configure(EntityTypeBuilder<SlotConfigurationActivationRow> builder)
    {
        builder.ToTable("SlotConfigurationActivations");
        builder.HasKey(row => row.ActivationId);
        builder
            .HasIndex(row => row.CommandMessageId)
            .IsUnique()
            .HasFilter("CommandMessageId IS NOT NULL");
        builder.HasIndex(row => new { row.AgvId, row.IssuedAt });
    }
}
