using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ConfigurationConsumerBindingRowConfiguration
    : IEntityTypeConfiguration<ConfigurationConsumerBindingRow>
{
    public void Configure(EntityTypeBuilder<ConfigurationConsumerBindingRow> builder)
    {
        builder.ToTable("ConfigurationConsumerBindings");
        builder.HasKey(row => new { row.ConsumerKind, row.ConsumerId, row.ObjectKind, row.ObjectId });
        builder.Property(row => row.ObjectKind).HasConversion<string>();
        builder.HasIndex(row => row.FrozenAt);
    }
}
