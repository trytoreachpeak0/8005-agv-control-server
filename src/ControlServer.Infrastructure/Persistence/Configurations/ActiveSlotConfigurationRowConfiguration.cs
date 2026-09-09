using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ActiveSlotConfigurationRowConfiguration
    : IEntityTypeConfiguration<ActiveSlotConfigurationRow>
{
    public void Configure(EntityTypeBuilder<ActiveSlotConfigurationRow> builder)
    {
        builder.ToTable("ActiveSlotConfigurations");
        builder.HasKey(row => row.AgvId);
        builder.HasIndex(row => row.Fingerprint);
    }
}
