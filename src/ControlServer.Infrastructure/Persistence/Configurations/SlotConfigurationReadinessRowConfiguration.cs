using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SlotConfigurationReadinessRowConfiguration
    : IEntityTypeConfiguration<SlotConfigurationReadinessRow>
{
    public void Configure(EntityTypeBuilder<SlotConfigurationReadinessRow> builder)
    {
        builder.ToTable("SlotConfigurationReadiness");
        builder.HasKey(row => row.AgvId);
    }
}
