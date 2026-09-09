using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AutoChargingRunRowConfiguration : IEntityTypeConfiguration<AutoChargingRunRow>
{
    public void Configure(EntityTypeBuilder<AutoChargingRunRow> builder)
    {
        builder.HasKey(row => row.ChargingRunId);
        builder.HasIndex(row => row.UpperId).IsUnique();
        builder.Property(row => row.Stage).HasConversion<string>();
    }
}
