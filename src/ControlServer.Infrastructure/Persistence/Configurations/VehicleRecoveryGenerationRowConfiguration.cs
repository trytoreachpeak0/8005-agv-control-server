using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class VehicleRecoveryGenerationRowConfiguration
    : IEntityTypeConfiguration<VehicleRecoveryGenerationRow>
{
    public void Configure(EntityTypeBuilder<VehicleRecoveryGenerationRow> builder)
    {
        builder.HasKey(row => row.AgvId);
    }
}
